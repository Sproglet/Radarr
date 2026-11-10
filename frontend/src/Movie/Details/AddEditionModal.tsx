import React, { useCallback, useMemo, useState } from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import { inputTypes, kinds } from 'Helpers/Props';
import MovieEdition, {
  EditionNamingPreview,
  EditionProposal,
} from 'Movie/MovieEdition';
import postEdition from 'Movie/postEdition';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

const previewColumns = [
  { name: 'name', label: () => translate('Edition'), isVisible: true },
  { name: 'path', label: () => translate('File'), isVisible: true },
];

interface Attribute {
  key: string;
  value: string;
}

interface AddEditionModalProps {
  isOpen: boolean;
  movieId: number;
  qualityProfileId: number;
  onCreated(edition: MovieEdition): void;
  onModalClose(): void;
}

function AddEditionContent({
  movieId,
  qualityProfileId,
  onCreated,
  onModalClose,
  isCreating,
  onCreatingChange,
}: Omit<AddEditionModalProps, 'isOpen'> & {
  isCreating: boolean;
  onCreatingChange(value: boolean): void;
}) {
  const [name, setName] = useState('');
  const [profileId, setProfileId] = useState(qualityProfileId);
  const [attributes, setAttributes] = useState<Attribute[]>([
    { key: 'cut', value: 'theatrical' },
  ]);
  const [rules, setRules] = useState('');
  const [preview, setPreview] = useState<EditionNamingPreview>();
  const [previewProposal, setPreviewProposal] = useState<EditionProposal>();
  const [error, setError] = useState<string>();
  const [isPreviewing, setIsPreviewing] = useState(false);
  const isBusy = isPreviewing || isCreating;
  const attributesValid = useMemo(() => {
    const keys = attributes.map((attribute) => attribute.key.toLowerCase());
    return (
      attributes.every(
        (attribute) => attribute.key.trim() && attribute.value.trim()
      ) && new Set(keys).size === keys.length
    );
  }, [attributes]);
  const proposal = useMemo<EditionProposal>(
    () => ({
      movieId,
      name,
      qualityProfileId: profileId,
      attributes: Object.fromEntries(attributes.map((a) => [a.key, a.value])),
      matchRules: rules.split('\n').filter((rule) => rule.trim()),
    }),
    [movieId, name, profileId, attributes, rules]
  );

  const previewCurrent = previewProposal === proposal;

  const invalidate = useCallback(() => {
    setPreview(undefined);
    setPreviewProposal(undefined);
    setError(undefined);
  }, []);
  const onNameChange = useCallback(
    ({ value }: { value: string }) => {
      setName(value);
      invalidate();
    },
    [invalidate]
  );
  const onProfileChange = useCallback(
    ({ value }: { value: number | string }) => {
      setProfileId(Number(value));
      invalidate();
    },
    [invalidate]
  );
  const onAttributesChange = useCallback(
    ({ value }: { value: Attribute[] }) => {
      setAttributes(value);
      invalidate();
    },
    [invalidate]
  );
  const onRulesChange = useCallback(
    ({ value }: { value: string }) => {
      setRules(value);
      invalidate();
    },
    [invalidate]
  );
  const onClose = useCallback(() => {
    if (!isCreating) {
      onModalClose();
    }
  }, [isCreating, onModalClose]);
  const onPreview = useCallback(async () => {
    setIsPreviewing(true);
    setPreview(undefined);
    setPreviewProposal(undefined);
    setError(undefined);
    try {
      setPreview(
        await postEdition<EditionNamingPreview>(
          '/movieedition/preview',
          proposal
        )
      );
      setPreviewProposal(proposal);
    } catch (requestError) {
      setError(
        requestError instanceof ApiError
          ? getErrorMessage(requestError, translate('EditionPreviewFailed')) ||
              translate('EditionPreviewFailed')
          : translate('EditionPreviewFailed')
      );
    } finally {
      setIsPreviewing(false);
    }
  }, [proposal]);
  const onCreate = useCallback(async () => {
    if (
      !previewCurrent ||
      !preview?.namingValid ||
      !preview.creationEnabled ||
      isBusy
    ) {
      return;
    }
    onCreatingChange(true);
    setError(undefined);
    try {
      const edition = await postEdition<MovieEdition>(
        '/movieedition',
        proposal
      );
      onCreated(edition);
    } catch (requestError) {
      setPreview(undefined);
      setError(
        requestError instanceof ApiError
          ? getErrorMessage(requestError, translate('EditionCreateFailed')) ||
              translate('EditionCreateFailed')
          : translate('EditionCreateFailed')
      );
    } finally {
      onCreatingChange(false);
    }
  }, [preview, previewCurrent, isBusy, proposal, onCreated, onCreatingChange]);

  return (
    <ModalContent onModalClose={onClose}>
      <ModalHeader>{translate('AddEdition')}</ModalHeader>
      <ModalBody>
        <Alert>{translate('AddEditionHelpText')}</Alert>
        <fieldset disabled={isBusy}>
          <FormGroup>
            <FormLabel>{translate('Name')}</FormLabel>
            <FormInputGroup
              type={inputTypes.TEXT}
              name="editionName"
              value={name}
              onChange={onNameChange}
            />
          </FormGroup>
          <FormGroup>
            <FormLabel>{translate('QualityProfile')}</FormLabel>
            <FormInputGroup
              type={inputTypes.QUALITY_PROFILE_SELECT}
              name="editionProfileId"
              value={profileId}
              onChange={onProfileChange}
            />
          </FormGroup>
          <FormGroup>
            <FormLabel>{translate('EditionAttributes')}</FormLabel>
            <FormInputGroup
              type={inputTypes.KEY_VALUE_LIST}
              name="editionAttributes"
              value={attributes}
              keyPlaceholder={translate('EditionAttribute')}
              valuePlaceholder={translate('EditionAttributeValue')}
              helpText={translate('EditionAttributesHelpText')}
              onChange={onAttributesChange}
            />
          </FormGroup>
          <FormGroup>
            <FormLabel>{translate('EditionMatchRules')}</FormLabel>
            <FormInputGroup
              type={inputTypes.TEXT_AREA}
              name="editionRules"
              value={rules}
              helpText={translate('EditionMatchRulesHelpText')}
              onChange={onRulesChange}
            />
          </FormGroup>
        </fieldset>
        {attributesValid ? null : (
          <Alert kind={kinds.DANGER}>
            {translate('EditionAttributesInvalid')}
          </Alert>
        )}
        {error ? <Alert kind={kinds.DANGER}>{error}</Alert> : null}
        {preview && previewCurrent ? (
          <>
            {preview.rejections.map((reason) => (
              <Alert key={reason} kind={kinds.DANGER}>
                {reason}
              </Alert>
            ))}
            {preview.samples.length ? (
              <Table columns={previewColumns}>
                <TableBody>
                  {preview.samples.map((sample) => (
                    <TableRow key={sample.movieEditionId ?? 'proposed'}>
                      <TableRowCell>{sample.name}</TableRowCell>
                      <TableRowCell>{sample.relativePath}</TableRowCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : null}
            <p>{translate('EditionNamingPreviewHelpText')}</p>
          </>
        ) : null}
      </ModalBody>
      <ModalFooter>
        <Button isDisabled={isCreating} onPress={onClose}>
          {translate('Cancel')}
        </Button>
        <Button
          isDisabled={isBusy || !name.trim() || !attributesValid || !profileId}
          onPress={onPreview}
        >
          {translate('PreviewEditionFiles')}
        </Button>
        <Button
          kind={kinds.PRIMARY}
          isDisabled={
            isBusy ||
            !previewCurrent ||
            !preview?.namingValid ||
            !preview.creationEnabled
          }
          onPress={onCreate}
        >
          {translate('AddEdition')}
        </Button>
      </ModalFooter>
    </ModalContent>
  );
}

function AddEditionModal({
  isOpen,
  onCreated,
  onModalClose,
  ...props
}: AddEditionModalProps) {
  const [isCreating, setIsCreating] = useState(false);
  const onClose = useCallback(() => {
    if (!isCreating) {
      onModalClose();
    }
  }, [isCreating, onModalClose]);
  const handleCreated = useCallback(
    (edition: MovieEdition) => {
      onCreated(edition);
      onModalClose();
    },
    [onCreated, onModalClose]
  );
  return (
    <Modal isOpen={isOpen} onModalClose={onClose}>
      <AddEditionContent
        {...props}
        isCreating={isCreating}
        onCreatingChange={setIsCreating}
        onCreated={handleCreated}
        onModalClose={onClose}
      />
    </Modal>
  );
}

export default AddEditionModal;
