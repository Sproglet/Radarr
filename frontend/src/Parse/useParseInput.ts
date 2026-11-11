import { useCallback, useEffect, useState } from 'react';
import { useDispatch } from 'react-redux';
import { clear, fetch } from 'Store/Actions/parseActions';
import { InputChanged } from 'typings/inputs';

function useParseInput() {
  const dispatch = useDispatch();
  const [title, setTitle] = useState('');
  const [movieEditionId, setMovieEditionId] = useState<number>();

  const onInputChange = useCallback(
    ({ value }: InputChanged<string>) => {
      setTitle(value);
      setMovieEditionId(undefined);
      dispatch(value.trim() ? fetch({ title: value.trim() }) : clear());
    },
    [dispatch]
  );

  const onClearPress = useCallback(() => {
    setTitle('');
    setMovieEditionId(undefined);
    dispatch(clear());
  }, [dispatch]);

  const onEditionChange = useCallback(
    ({ value }: InputChanged<string>) => {
      const id = Number(value) || undefined;
      setMovieEditionId(id);
      dispatch(fetch({ title: title.trim(), movieEditionId: id }));
    },
    [dispatch, title]
  );

  useEffect(
    () => () => {
      dispatch(clear());
    },
    [dispatch]
  );

  return {
    title,
    movieEditionId,
    onInputChange,
    onClearPress,
    onEditionChange,
  };
}

export default useParseInput;
