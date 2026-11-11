import React, { useCallback, useState } from 'react';
import Alert from 'Components/Alert';
import SelectInput from 'Components/Form/SelectInput';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import useApiQuery, { ApiError } from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props';
import MovieEdition from 'Movie/MovieEdition';
import postEdition from 'Movie/postEdition';
import { InputChanged } from 'typings/inputs';
import QualityProfile from 'typings/QualityProfile';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

export interface ProfileSelection {
  target: MovieEdition;
  movieMonitored: boolean;
}

interface SetEditionQualityProfileModalProps {
  movieId: number;
  selection: ProfileSelection;
  onSelected(): void;
  onError(message: string): void;
  onClose(): void;
}

function SetEditionQualityProfileModal({
  movieId,
  selection,
  onSelected,
  onError,
  onClose,
}: SetEditionQualityProfileModalProps) {
  const [profileId, setProfileId] = useState(selection.target.qualityProfileId);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const { data, error, isPending } = useApiQuery<QualityProfile[]>({
    path: '/qualityprofile',
    queryOptions: { staleTime: 0, refetchOnMount: 'always', retry: false },
  });
  const choiceValid = data?.some((profile) => profile.id === profileId);
  const choices =
    data?.map((profile) => ({ key: profile.id, value: profile.name })) ?? [];
  if (!choices.some((profile) => profile.key === profileId)) {
    choices.unshift({
      key: profileId,
      value: translate('EditionProfileUnavailable', { id: profileId }),
    });
  }
  const onProfileChange = useCallback(
    ({ value }: InputChanged<string>) => setProfileId(Number(value)),
    []
  );
  const onCancel = useCallback(() => {
    if (!isSubmitting) onClose();
  }, [isSubmitting, onClose]);
  const onSave = useCallback(async () => {
    if (isSubmitting || !choiceValid) return;
    setIsSubmitting(true);
    try {
      await postEdition<MovieEdition>(
        `/movieedition/${selection.target.id}/qualityprofile`,
        {
          movieId,
          qualityProfileId: profileId,
          expectedMovieMonitored: selection.movieMonitored,
          expectedEditionMonitored: selection.target.monitored,
          expectedMovieFileId: selection.target.movieFileId ?? null,
          expectedQualityProfileId: selection.target.qualityProfileId,
          expectedRuleRevision: selection.target.ruleRevision,
          expectedName: selection.target.name,
        }
      );
      onSelected();
    } catch (requestError) {
      onError(
        requestError instanceof ApiError
          ? getErrorMessage(requestError, translate('EditionProfileFailed')) ||
              translate('EditionProfileFailed')
          : translate('EditionProfileFailed')
      );
    } finally {
      setIsSubmitting(false);
    }
  }, [
    isSubmitting,
    choiceValid,
    movieId,
    profileId,
    selection,
    onSelected,
    onError,
  ]);
  return (
    <Modal isOpen={true} onModalClose={onCancel}>
      <ModalContent onModalClose={onCancel}>
        <ModalHeader>{translate('ChangeEditionProfile')}</ModalHeader>
        <ModalBody>
          <p>
            {translate('EditionProfileScopeHelpText', {
              edition: selection.target.name,
            })}
          </p>
          {isPending ? <LoadingIndicator /> : null}
          {error ? (
            <Alert kind={kinds.DANGER}>
              {translate('EditionProfileLoadFailed')}
            </Alert>
          ) : null}
          <label>
            {translate('QualityProfile')}
            <SelectInput
              name="editionQualityProfile"
              value={profileId}
              values={choices}
              isDisabled={isPending || isSubmitting || !!error}
              onChange={onProfileChange}
            />
          </label>
        </ModalBody>
        <ModalFooter>
          <Button isDisabled={isSubmitting} onPress={onCancel}>
            {translate('Cancel')}
          </Button>
          <SpinnerButton
            isSpinning={isSubmitting}
            isDisabled={!choiceValid || !!error}
            onPress={onSave}
          >
            {translate('Save')}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

export default SetEditionQualityProfileModal;
