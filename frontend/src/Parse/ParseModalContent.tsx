import React from 'react';
import { useSelector } from 'react-redux';
import TextInput from 'Components/Form/TextInput';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import ParseEditionSelector from './ParseEditionSelector';
import ParseResult from './ParseResult';
import parseStateSelector from './parseStateSelector';
import useParseInput from './useParseInput';
import styles from './ParseModalContent.css';

interface ParseModalContentProps {
  onModalClose: () => void;
}

function ParseModalContent(props: ParseModalContentProps) {
  const { onModalClose } = props;
  const { isFetching, error, item } = useSelector(parseStateSelector());

  const {
    title,
    movieEditionId,
    onInputChange,
    onClearPress,
    onEditionChange,
  } = useParseInput();

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>{translate('TestParsing')}</ModalHeader>

      <ModalBody>
        <div className={styles.inputContainer}>
          <div className={styles.inputIconContainer}>
            <Icon name={icons.PARSE} size={20} />
          </div>

          <TextInput
            className={styles.input}
            name="title"
            value={title}
            placeholder="eg. Movie.Title.2020.720p.HDTV-RlsGroup"
            autoFocus={true}
            onChange={onInputChange}
          />

          <Button className={styles.clearButton} onPress={onClearPress}>
            <Icon name={icons.REMOVE} size={20} />
          </Button>
        </div>

        <ParseEditionSelector
          movieId={item.movie?.id}
          movieEditionId={movieEditionId}
          isFetching={isFetching}
          onChange={onEditionChange}
        />

        {isFetching ? <LoadingIndicator /> : null}

        {!isFetching && !!error ? (
          <div className={styles.message}>
            <div className={styles.helpText}>
              {translate('ParseModalErrorParsing')}
            </div>
            <div>{getErrorMessage(error)}</div>
          </div>
        ) : null}

        {!isFetching && title && !error && !item.parsedMovieInfo ? (
          <div className={styles.message}>
            {translate('ParseModalUnableToParse')}
          </div>
        ) : null}

        {!isFetching && !error && item.parsedMovieInfo ? (
          <ParseResult item={item} />
        ) : null}

        {title ? null : (
          <div className={styles.message}>
            <div className={styles.helpText}>
              {translate('ParseModalHelpText')}
            </div>
            <div>{translate('ParseModalHelpTextDetails')}</div>
          </div>
        )}
      </ModalBody>

      <ModalFooter>
        <Button onPress={onModalClose}>{translate('Close')}</Button>
      </ModalFooter>
    </ModalContent>
  );
}

export default ParseModalContent;
